// File: OpenModulePlatform.Portal/Pages/Admin/Navigation.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class NavigationModel : OmpPortalPageModel
{
    private readonly LinkBoxRepository _linkBoxes;

    public NavigationModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        LinkBoxRepository linkBoxes)
        : base(options, rbac)
    {
        _linkBoxes = linkBoxes;
    }

    // Registered boxes plus any box keys that only exist in storage (e.g. a
    // module seeded rows before Portal learned about the box).
    public IReadOnlyList<PortalLinkBoxDefinition> Boxes { get; private set; } = Array.Empty<PortalLinkBoxDefinition>();
    public string SelectedBoxKey { get; private set; } = PortalLinkBoxes.AdminQuickLinksKey;
    public PortalLinkBoxDefinition? SelectedDefinition { get; private set; }
    public IReadOnlyList<LinkBoxItemRow> UserLinks { get; private set; } = Array.Empty<LinkBoxItemRow>();

    [BindProperty]
    public LinkInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(string? box, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(box, ct);
        SetTitles("Navigation");
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(string box, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        ValidateInput();
        if (!ModelState.IsValid)
        {
            await LoadAsync(box, ct);
            SetTitles("Navigation");
            return Page();
        }

        await _linkBoxes.AddItemAsync(
            box,
            Input.Label.Trim(),
            Input.Url.Trim(),
            string.IsNullOrWhiteSpace(Input.Group) ? null : Input.Group.Trim(),
            ct);

        StatusMessage = T("Link added.");
        return RedirectToPage("Navigation", new { box });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string box, long id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await _linkBoxes.DeleteItemAsync(id, ct);
        StatusMessage = T("Link removed.");
        return RedirectToPage("Navigation", new { box });
    }

    private async Task LoadAsync(string? box, CancellationToken ct)
    {
        var boxes = new List<PortalLinkBoxDefinition>(PortalLinkBoxes.All);
        foreach (var storedKey in await _linkBoxes.GetBoxKeysAsync(ct))
        {
            if (boxes.All(item => !string.Equals(item.BoxKey, storedKey, StringComparison.OrdinalIgnoreCase)))
            {
                boxes.Add(new PortalLinkBoxDefinition(storedKey, storedKey, Array.Empty<PortalCodeLink>()));
            }
        }

        Boxes = boxes;
        SelectedBoxKey = boxes.FirstOrDefault(item => string.Equals(item.BoxKey, box, StringComparison.OrdinalIgnoreCase))?.BoxKey
            ?? PortalLinkBoxes.AdminQuickLinksKey;
        SelectedDefinition = Boxes.FirstOrDefault(item => string.Equals(item.BoxKey, SelectedBoxKey, StringComparison.OrdinalIgnoreCase));
        UserLinks = await _linkBoxes.GetItemsAsync(SelectedBoxKey, ct);
    }

    private void ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(Input.Label))
        {
            ModelState.AddModelError("Input.Label", T("Label is required."));
        }
        else if (Input.Label.Trim().Length > 200)
        {
            ModelState.AddModelError("Input.Label", T("Label can be at most 200 characters."));
        }

        var url = Input.Url?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            ModelState.AddModelError("Input.Url", T("URL is required."));
        }
        else if (url.Length > 400)
        {
            ModelState.AddModelError("Input.Url", T("URL can be at most 400 characters."));
        }
        else if (!IsSafeRelativeUrl(url))
        {
            ModelState.AddModelError("Input.Url", T("Only relative URLs within this site are allowed, e.g. /admin/hosts."));
        }

        if (!string.IsNullOrWhiteSpace(Input.Group) && Input.Group.Trim().Length > 100)
        {
            ModelState.AddModelError("Input.Group", T("Group can be at most 100 characters."));
        }
    }

    // Same-origin guard: only app-relative paths are storable, so a stored
    // link can never navigate off-site or smuggle a scheme (open redirect /
    // javascript: XSS). Rendering prefixes "~" for virtual app paths.
    private static bool IsSafeRelativeUrl(string url)
    {
        return url.StartsWith('/')
            && !url.StartsWith("//", StringComparison.Ordinal)
            && !url.StartsWith("/\\", StringComparison.Ordinal)
            && Uri.TryCreate(url, UriKind.Relative, out _);
    }

    public sealed class LinkInput
    {
        public string Label { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? Group { get; set; }
    }
}
